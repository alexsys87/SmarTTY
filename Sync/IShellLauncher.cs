using SmarTTY.Ssh;

namespace SmarTTY.Sync;

public interface IShellLauncher
{
    string Command { get; }

    void Prepare(SshShellChannel sshShellChannel, string text);
}
