using SmarTTY.Ssh;

namespace SmarTTY.GUI.Backend;

internal class TerminalTag
{
    public SshShellChannel Channel;

    public SessionWrapper Session;

    /// <summary>
    /// True when reopening the shell after a reconnect failed: <see cref="Channel"/> is then a closed channel of the old
    /// connection. The next key press tries again (see SessionWrapper.RetryShell) instead of writing to it.
    /// </summary>
    public bool ShellOpenFailed;
}
