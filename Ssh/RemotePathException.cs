using SmarTTY;
using SmarTTY.IO;
using System;

namespace SmarTTY.Ssh;

public class RemotePathException : Exception, IProjectAwareException
{
    public readonly RemoteFileContext Context;

    public readonly RemotePathProblem Problem;

    public RemotePathException(RemoteFileContext remoteFileContext, RemotePathProblem remotePathProblem, string projectName = null)
        : base(RemotePathException.BuildMessage(remoteFileContext, remotePathProblem, projectName))
    {
        this.Context = remoteFileContext;
        this.Problem = remotePathProblem;
    }

    private static string BuildMessage(RemoteFileContext remoteFileContext, RemotePathProblem remotePathProblem, string projectName)
    {
        string extraInfo = "";
        if (!string.IsNullOrEmpty(projectName))
        {
            extraInfo = " Related project name: " + projectName + ".";
        }
        return GetProblemText(remoteFileContext, remotePathProblem) + extraInfo;
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

    public Exception WithProjectName(string projectName)
    {
        return new RemotePathException(Context, Problem, projectName);
    }
}
