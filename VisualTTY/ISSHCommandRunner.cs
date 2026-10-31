using System;

namespace VisualTTY;

public interface ISSHCommandRunner
{
    string[] RunCommand(string command);
}

/// <summary>
/// A runner that can abandon a command that has not finished in time (e.g. "opkg update" on a host without internet).
/// </summary>
public interface ISSHCommandRunnerWithTimeout : ISSHCommandRunner
{
    /// <summary>Like <see cref="ISSHCommandRunner.RunCommand(string)"/>, but returns null when the command has not finished within <paramref name="timeout"/>.</summary>
    string[] RunCommand(string command, TimeSpan timeout);
}
