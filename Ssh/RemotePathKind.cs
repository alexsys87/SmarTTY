namespace SmarTTY.Ssh;

/// <summary>
/// What a remote path refers to, as reported by <see cref="SshSession.GetRemotePathKind"/>.
/// </summary>
public enum RemotePathKind
{
    /// <summary>The check could not be run (no shell, connection problem).</summary>
    Unknown,
    Missing,
    File,
    Directory
}
