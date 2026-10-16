using ManagedLibSSH2;

namespace SmarTTY.Sync;

public interface IShellLauncher
{
    string Command { get; }

    void Prepare(SshShellChannel sshShellChannel, string text);
}
