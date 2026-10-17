namespace SmarTTY.Ssh;

public struct RemoteFileContext(string path, string hostName, string userName)
{
    public readonly string Path = path;

    public readonly string HostName = hostName;

    public readonly string UserName = userName;
}
