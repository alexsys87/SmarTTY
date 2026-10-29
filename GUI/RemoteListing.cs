using SmarTTY.IO;
using System.Collections.Generic;

namespace SmarTTY.GUI;

/// <summary>
/// Remote directory listing helpers shared by the file and folder browsers: the working directory of a session and
/// the "directories first, then files" ordering the <c>ls</c> output is shown in.
/// </summary>
internal static class RemoteListing
{
    /// <summary>The session's working directory (<c>pwd</c>), or null when it could not be read.</summary>
    internal static string GetWorkingDirectory(RemoteCommandStarter callback)
    {
        IRemoteCommand remoteCommand = null;
        callback("pwd", "", ref remoteCommand);
        using (remoteCommand)
        {
            if (remoteCommand.WaitForExit(5000))
            {
                List<string> output = remoteCommand.OutputLines;
                if (output != null && output.Count > 0)
                {
                    return output[0];
                }
            }
        }
        return null;
    }

    /// <summary>
    /// The text for a listing that failed, the same whether the answer was ready at once or arrived while the dialog was polling.
    /// The text <c>ls</c> printed to stderr wins. Exit status -1 is what a channel closed by a lost connection reports (also a
    /// command killed by a signal): it is no verdict on the directory, so it must not read as "Non-existent directory?".
    /// </summary>
    internal static string DescribeFailure(int exitCode, string errorText = null)
    {
        if (!string.IsNullOrEmpty(errorText))
        {
            return errorText;
        }
        return exitCode == -1
            ? "The listing was interrupted, most likely because the connection to the server was lost. Try again after reconnecting."
            : "'ls -1' failed with code: " + exitCode + ". Non-existent directory?";
    }

    /// <summary>
    /// Reorders an <c>ls</c> listing so that directories come first. With <paramref name="isPrefixed"/> the entries
    /// carry the <c>ls -F</c> type prefix ("d " for a directory), otherwise they end in "/" when they are one.
    /// </summary>
    internal static List<string> SortEntries(List<string> entries, bool isPrefixed = false)
    {
        List<string> dirList = new List<string>();
        List<string> fileList = new List<string>();
        foreach (string item in entries)
        {
            if (isPrefixed)
            {
                if (item.StartsWith("d ") || item.StartsWith("ld "))
                {
                    dirList.Add(item);
                }
                else
                {
                    fileList.Add(item);
                }
            }
            else if (item.EndsWith("/"))
            {
                dirList.Add(item);
            }
            else
            {
                fileList.Add(item);
            }
        }
        dirList.AddRange(fileList);
        return dirList;
    }
}
