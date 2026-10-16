using SmarTTY;
using SmarTTY.IO;
using System;

namespace SmarTTY.Ssh;

public class RemotePathException : Exception, IProjectAwareException
{
    public readonly RemoteFileContext Context;

    public readonly RemotePathProblem Problem;

    public RemotePathException(RemoteFileContext remoteFileContext, RemotePathProblem remotePathProblem, string text = null)
        : base(RemotePathException.BuildMessage(remoteFileContext, remotePathProblem, text))
    {
        this.Context = remoteFileContext;
        this.Problem = remotePathProblem;
    }

    private static string BuildMessage(RemoteFileContext remoteFileContext, RemotePathProblem remotePathProblem, string text2)
    {
        string text = "";
        if (!string.IsNullOrEmpty(text2))
        {
            text = " Related project name: " + text2 + ".";
        }
        return GetProblemText(remoteFileContext, remotePathProblem) + text;
    }

    private static string GetProblemText(RemoteFileContext remoteFileContext, RemotePathProblem remotePathProblem)
    {
        return remotePathProblem switch
        {
            RemotePathProblem.EmptyPath => "Remote file path is empty.",
            RemotePathProblem.EndsWithSlash => "Remote file path ('" + remoteFileContext.Path + "') ends with '/'. The path should point to a file, not directory.",
            RemotePathProblem.NotAbsolute => "Remote file path ('" + remoteFileContext.Path + "') is not absolute.",
            RemotePathProblem.FileNotWritable => "The '" + remoteFileContext.Path + "' file on '" + remoteFileContext.HostName + "' is not writable by '" + remoteFileContext.UserName + "'. Please double-check file permissions or try deleting it with sudo.",
            RemotePathProblem.DirectoryNotWritable => "The '" + RemotePathUtils.GetDirectoryName(remoteFileContext.Path) + "' directory on '" + remoteFileContext.HostName + "' is not writable by '" + remoteFileContext.UserName + "'. Please double-check file permissions or try deleting it with sudo.",
            _ => "Failed to upload " + remoteFileContext.Path + ".",
        };
    }

    public Exception WithProjectName(string text)
    {
        return new RemotePathException(Context, Problem, text);
    }
}
