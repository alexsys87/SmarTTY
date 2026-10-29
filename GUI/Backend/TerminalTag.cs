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

    /// <summary>
    /// Keeps the writes into <see cref="Channel"/> in order: the commands the program types in and the key presses. It belongs to
    /// the terminal, not to one channel: the channel is replaced after a reconnect, the order of the writes is not.
    /// </summary>
    public readonly TerminalInputQueue Writes = new TerminalInputQueue();
}
